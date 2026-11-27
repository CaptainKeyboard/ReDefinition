using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The own clouds in what the sea and the land mirror (master plan 46.1 part 4; the owner 11-26: "Klar soll das Meer die Wolkendecke spiegeln"). With
    // Scatterer's sky reflections off the ocean reads KSP's flight reflection probe, and the land's environment light is that probe too (PRUEFPROTOKOLL 293,
    // 312); an own probe of mode Custom with a higher importance in KSP's probe's box replaces it for both (314). Its cube is KSP's probe's cube with the own
    // clouds composited over it: every few frames a part of a face is copied from KSP's probe and the layer pass's march is drawn through it (pass 8: the face's
    // basis, no scene depth, no reconstruction, with the display stage; the pacing below); after the last part of the sixth face the mips are made again. KSP's probe itself is not touched; while
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

        // THE PACING (PRUEFPROTOKOLL 365; the owner 11-27: a view below 48 fps is to be explained and fixed whichever part of the frame holds the time). A whole face
        // every second frame was 2 to 4 ms of GPU time a frame outside the cameras (the GPU table's "between scripts and cameras": 1.8 ms before the probe, 4 to 6
        // with it) in a lump of 4 to 8 ms on every second frame. Now one of a face's PartsPerFace parts (each a range of columns) every FramesPerStep frames: a quarter of the
        // work a frame, half the lump, the cube fresh after TurnFrames frames (about a second; the clouds a mirror shows move far less in it).
        // A class of its own: the probe's own statics hold shader property ids, which only the engine can make (a test that touched them could not load the type).
        internal static class Pacing
        {
            internal const int FramesPerStep = 4, PartsPerFace = 2;
            internal const int TurnFrames = FramesPerStep * PartsPerFace * 6;

            // Whether a part is drawn in the frame.
            internal static bool DrawsAt(int frameCount)
            {
                return frameCount % FramesPerStep == 0;
            }

            // The columns of a part of a face n texels wide: parts side by side (columns, not rows: a row range would depend on which way up the scissor and the
            // copy count a render texture's rows), the last taking the rest.
            internal static void PartColumns(int part, int n, out int x0, out int width)
            {
                int w = System.Math.Max(1, n / PartsPerFace);
                x0 = System.Math.Min(part * w, System.Math.Max(n - 1, 0));
                width = part >= PartsPerFace - 1 ? System.Math.Max(n - x0, 1) : System.Math.Min(w, System.Math.Max(n - x0, 1));
            }

            // The step after (face, part): the next part, then the next face.
            internal static void Next(ref int face, ref int part)
            {
                part++;
                if (part < PartsPerFace) return;
                part = 0;
                face = (face + 1) % 6;
            }
        }

        private ReflectionProbe ksp, own;
        private GameObject holder;
        private RenderTexture cube;
        private Texture2D noDepth;
        // Made on first use (the module is built in the tests, outside the engine).
        private CommandBuffer cb;
        private MaterialPropertyBlock block;
        private int face, part;
        private float nextScan;
        private bool said, filled, dumped;
        private float dumpFrom = -1f;
        // Measurement (PRUEFPROTOKOLL 319): light.dumpSkyProbe 1 writes the cube's six faces (mip 0) as PNGs into the series pictures' folder, once after a full turn.
        internal const string TuneDump = "light.dumpSkyProbe";
        // Measurement (PRUEFPROTOKOLL 320, with layers.devVariant 1): light.testBelowWhite 1 fills the cube below the horizon white, 2 the whole cube (no clouds).
        internal const string TuneBelowWhite = "light.testBelowWhite";

        private void Dump()
        {
            dumped = true;
            int n = cube.width;
            RenderTexture tmp = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            RenderTexture before = RenderTexture.active;
            try
            {
                for (int f = 0; f < 6; f++)
                {
                    Graphics.CopyTexture(cube, f, 0, tmp, 0, 0);
                    RenderTexture.active = tmp;
                    t.ReadPixels(new Rect(0, 0, n, n), 0, 0);
                    t.Apply(false);
                    string file = System.IO.Path.Combine(SeriesPictures.Folder, "skyprobe-face" + f + ".png");
                    System.IO.File.WriteAllBytes(file, t.EncodeToPNG());
                }
                AtmospheresLog.Say("light measurement: the sky probe's six faces written to " + SeriesPictures.Folder + " (skyprobe-face0..5.png: +X -X +Y -Y +Z -Z)");
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(tmp);
                Object.Destroy(t);
            }
        }

        internal bool Active { get { return own != null && own.enabled; } }

        // The cube the sea mirror reads (stage D, OwnCloudSeaMirror): null while the probe is off.
        internal RenderTexture Cube { get { return Active ? cube : null; } }

        // From the own clouds' frame, after the layer pass's material holds the frame's values: keeps the own probe on KSP's and draws a part of a face every
        // FramesPerStep frames (the pacing above; a face a frame cost about 3 fps, PRUEFPROTOKOLL 316). skyShare: the diffuse share under the clouds at the camera
        // (OwnCloudModule's), which the copied clear scene is darkened to before the clouds go over it.
        internal void Update(Material layers, float skyShare)
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
            // CopyTexture needs the same size and format (KSP's probe is LDR ARGB32).
            if (source is RenderTexture && ((RenderTexture)source).format != RenderTextureFormat.ARGB32)
            {
                if (!said)
                {
                    said = true;
                    AtmospheresLog.WarnOnce("own clouds: KSP's flight probe is " + ((RenderTexture)source).format + ", not ARGB32: the sky probe stays off");
                }
                Off();
                return;
            }
            if (cube == null || cube.width != source.width)
            {
                if (cube != null)
                {
                    cube.Release();
                    Object.Destroy(cube);
                }
                cube = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "ReDefinition own sky probe", dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = false
                };
                cube.Create();
                face = 0;
                part = 0;
                filled = false;
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
            // On only once every face holds the clouds (the idea check 319: an empty cube for the first turn is black in the sea).
            own.enabled = filled;
            holder.transform.position = ksp.transform.position;

            if (!Pacing.DrawsAt(Time.frameCount)) return;
            if (cb == null) cb = new CommandBuffer { name = "ReDefinition own sky probe" };
            if (block == null) block = new MaterialPropertyBlock();
            cb.Clear();
            int x0, width;
            Pacing.PartColumns(part, cube.width, out x0, out width);
            DrawFace(cb, layers, source, face, x0, width, holder.transform.position, Tuning.Get(TuneCubeDirs, 0f) > 0.5f, skyShare);
            bool turnDone = face == 5 && part == Pacing.PartsPerFace - 1;
            if (turnDone) cb.GenerateMips(cube);
            Graphics.ExecuteCommandBuffer(cb);
            if (turnDone)
            {
                filled = true;
                // The dump waits 3 s after the key turns on, then for this full turn: a pin's maps settle about 2.5 s (PRUEFPROTOKOLL 320: the first dump came at
                // the first face 5 after the key and held five faces from before the pin).
                if (Tuning.Get(TuneDump, 0f) > 0.5f)
                {
                    if (dumpFrom < 0f) dumpFrom = Time.realtimeSinceStartup + 3f;
                    else if (!dumped && Time.realtimeSinceStartup >= dumpFrom) Dump();
                }
                else
                {
                    dumped = false;
                    dumpFrom = -1f;
                }
            }
            Pacing.Next(ref face, ref part);
            if (!said)
            {
                said = true;
                AtmospheresLog.Say("own clouds: the sky probe mirrors the own clouds (" + cube.width + " per face, a face's " + Pacing.PartsPerFace + " parts one every " + Pacing.FramesPerStep
                                   + " frames, a turn " + Pacing.TurnFrames + " frames, importance " + own.importance + " over KSP's flight probe)");
            }
        }

        // One part of a face: the columns x0 .. x0 + width of KSP's face copied, darkened and marched (the scissor keeps the two draws to them; the viewport stays the
        // whole face, so a texel's ray is the one it has when the face is drawn at once).
        private void DrawFace(CommandBuffer c, Material layers, Texture source, int f, int x0, int width, Vector3 at, bool testDirs, float skyShare)
        {
            int n = cube.width;
            c.CopyTexture(source, f, 0, x0, 0, width, n, cube, f, 0, x0, 0);
            c.SetRenderTarget(cube, 0, (CubemapFace)f);
            c.EnableScissorRect(new Rect(x0, 0, width, n));
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
            // KSP's face holds a clear, sunlit scene; under the clouds its land and sea are lit by the diffuse share they pass on (316, 318): darkened first, with the
            // face's basis set (its rays decide what lies below the horizon; the idea check 319 found the first version reading the main camera's), the clouds after.
            float white = Mathf.Round(Tuning.Get(TuneBelowWhite, 0f));
            block.SetVector(IdSkyProbeP, new Vector4(0f, 1f, Mathf.Clamp01(skyShare), white));
            c.DrawProcedural(Matrix4x4.identity, layers, PassFace, MeshTopology.Triangles, 3, 1, block);
            // light.testBelowWhite 2: the whole face white, no clouds over it.
            if (white < 2f)
            {
                // light.testCubeDirs (the test variant: layers.devVariant 1): the faces hold their rays' directions as colours, drawn by the same pass and basis as the clouds.
                block.SetVector(IdSkyProbeP, new Vector4(testDirs ? 1f : 0f, 0f, 0f, 0f));
                c.DrawProcedural(Matrix4x4.identity, layers, PassFace, MeshTopology.Triangles, 3, 1, block);
            }
            c.DisableScissorRect();
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
            if (cube != null)
            {
                cube.Release();
                Object.Destroy(cube);
            }
            cube = null;
            if (noDepth != null) Object.Destroy(noDepth);
            noDepth = null;
        }
    }
}
