using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Core;
using ReDefinition.Raytracing;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // The Debug tab's screen recording also writes the frame's stages, for following a
    // fault through the pipeline away from the game: for a few consecutive frames from
    // the first one in which the camera turns by more than a degree, the image the
    // upscaler or Ray Reconstruction takes, the image it gives, and the raytracing's
    // signals traced in that frame (RtCamera: the sun's visibility with the kept view
    // depth in w, the diffuse light, the reflections, the light added without Deferred),
    // and EVE's clouds where it draws them.
    // Written into ReDefinitionCaptures\<time>-stages beside the other dumps; meta.txt
    // describes them.
    //
    // The frames dumped are the ones the recording is about, so they cost no more than a
    // copy on the GPU each: the textures the copies go into are made while the dump waits
    // for the turn, and read back one a frame after the last copy, through one buffer
    // kept for all, which a thread of its own writes out before the next is read. Read
    // back and copied into a new array each, seven textures a frame -- some hundred
    // megabytes -- went with frames slowing from 17 to 138 ms (input dump of 2026-10-01
    // 22:03).
    internal static class StageDump
    {
        private const int Frames = 4;
        private const float TurnDegrees = 1f;
        private const int WaitFrames = 600;

        private sealed class Copy
        {
            internal string Path;
            internal RenderTexture Texture;
        }

        private static string folder;
        private static int taken;
        private static int waited;
        private static bool haveForward;
        private static Vector3 lastForward;
        private static readonly StringBuilder meta = new StringBuilder();

        // Per frame to take and stage, the texture its copy goes into; made while waiting.
        private static readonly Dictionary<string, RenderTexture>[] prepared =
        {
            new Dictionary<string, RenderTexture>(), new Dictionary<string, RenderTexture>(),
            new Dictionary<string, RenderTexture>(), new Dictionary<string, RenderTexture>(),
        };
        private static readonly Queue<Copy> toRead = new Queue<Copy>();
        private static byte[] buffer;
        // 1 while a read-back is in flight or its bytes are not yet written.
        private static int busy;
        private static string metaPath;
        private static string metaText;

        // EVE's volumetric clouds as its reconstruction left them this frame, where EVE
        // draws them: the layer the halo and trail around the rocket may come from.
        private static readonly int CloudsId = Shader.PropertyToID("scattererReconstructedCloud");

        private static RenderTexture Clouds()
        {
            return Shader.GetGlobalTexture(CloudsId) as RenderTexture;
        }

        internal static void Arm()
        {
            ReleasePrepared();
            folder = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "ReDefinitionCaptures"),
                                  System.DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-stages");
            taken = 0;
            waited = 0;
            haveForward = false;
            meta.Length = 0;
            meta.Append("# frame unityFrame reconstructing jitterX jitterY worldToCamera(16) projection(16, without jitter),"
                        + " Unity's [row, column] row by row; then each texture: name width height format\n");
        }

        // After the upscaler's dispatch: input the image it took, output the image it gave.
        internal static void AfterDispatch(Camera cam, Matrix4x4 unjittered, Vector2 jitter, bool reconstructing,
                                           RenderTexture input, RenderTexture output)
        {
            ReadNext();
            if (folder == null || cam == null) return;
            RtCamera raytraced = RtCamera.Current;
            if (raytraced != null && raytraced.Camera != cam) raytraced = null;
            Vector3 forward = cam.transform.forward;
            if (taken == 0)
            {
                bool turning = haveForward && Vector3.Angle(forward, lastForward) > TurnDegrees;
                lastForward = forward;
                haveForward = true;
                if (!turning && ++waited < WaitFrames)
                {
                    Prepare(input, output, raytraced);
                    return;
                }
                Directory.CreateDirectory(folder);
                Debug.Log(Log.Tag + " Stage dump: " + (turning ? "the camera turns" : "no turn within " + WaitFrames
                                                                                  + " frames") + ", " + Frames
                          + " frames into " + folder + ".");
            }

            int n = taken++;
            meta.Append(n).Append(' ').Append(Time.frameCount).Append(' ').Append(reconstructing ? 1 : 0).Append(' ')
                .Append(jitter.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(jitter.y.ToString("R", CultureInfo.InvariantCulture));
            Append(cam.worldToCameraMatrix);
            Append(unjittered);
            Take(n, "input", input);
            Take(n, "output", output);
            Take(n, "clouds", Clouds());
            if (raytraced != null)
            {
                Take(n, "sun", raytraced.SunTexture);
                Take(n, "diffuse", raytraced.DiffuseTexture);
                Take(n, "specular", raytraced.SpecularTexture);
                Take(n, "forward", raytraced.ForwardTexture);
                Take(n, "depth", raytraced.DepthTexture);
            }
            meta.Append('\n');
            if (taken < Frames) return;

            // Written after every texture: the folder is complete once meta.txt is there.
            metaPath = Path.Combine(folder, "meta.txt");
            metaText = meta.ToString();
            folder = null;
            ReleasePrepared();
        }

        private static void Prepare(RenderTexture input, RenderTexture output, RtCamera raytraced)
        {
            for (int n = 0; n < Frames; n++)
            {
                Prepare(n, "input", input);
                Prepare(n, "output", output);
                Prepare(n, "clouds", Clouds());
                if (raytraced == null) continue;
                Prepare(n, "sun", raytraced.SunTexture);
                Prepare(n, "diffuse", raytraced.DiffuseTexture);
                Prepare(n, "specular", raytraced.SpecularTexture);
                Prepare(n, "forward", raytraced.ForwardTexture);
                Prepare(n, "depth", raytraced.DepthTexture);
            }
        }

        private static bool Fits(RenderTexture copy, RenderTexture source)
        {
            return copy != null && copy.IsCreated() && copy.width == source.width && copy.height == source.height
                   && copy.format == source.format;
        }

        private static void Prepare(int n, string name, RenderTexture source)
        {
            if (source == null || !source.IsCreated()) return;
            RenderTexture copy;
            prepared[n].TryGetValue(name, out copy);
            if (Fits(copy, source)) return;
            RenderTextures.Release(ref copy);
            RenderTextureDescriptor desc = source.descriptor;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;
            desc.enableRandomWrite = false;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            copy = new RenderTexture(desc) { name = "ReDefinition_StageDump_" + name };
            copy.Create();
            prepared[n][name] = copy;
            int bytes = source.width * source.height * 16;
            if (buffer == null || buffer.Length < bytes) buffer = new byte[bytes];
        }

        private static void Take(int n, string name, RenderTexture source)
        {
            if (source == null || !source.IsCreated()) return;
            RenderTexture copy;
            prepared[n].TryGetValue(name, out copy);
            // Not prepared -- the camera turned in the first frame waited -- made now.
            if (!Fits(copy, source))
            {
                Prepare(n, name, source);
                prepared[n].TryGetValue(name, out copy);
            }
            prepared[n].Remove(name);
            if (!Fits(copy, source)) return;
            Graphics.CopyTexture(source, copy);
            meta.Append(' ').Append(name).Append(' ').Append(source.width).Append(' ').Append(source.height).Append(' ')
                .Append(source.format);
            toRead.Enqueue(new Copy
            {
                Path = Path.Combine(folder, name + "-" + n.ToString("00", CultureInfo.InvariantCulture) + ".bin"),
                Texture = copy,
            });
        }

        // One copy read back at a time, once the one before is written; meta.txt last.
        private static void ReadNext()
        {
            if (Volatile.Read(ref busy) != 0) return;
            if (toRead.Count == 0)
            {
                if (metaPath == null) return;
                string path = metaPath;
                byte[] text = Encoding.ASCII.GetBytes(metaText);
                metaPath = null;
                metaText = null;
                ThreadPool.QueueUserWorkItem(_ => TryWrite(path, text, text.Length));
                return;
            }
            Copy next = toRead.Dequeue();
            Volatile.Write(ref busy, 1);
            AsyncGPUReadback.Request(next.Texture, 0, request =>
            {
                RenderTexture texture = next.Texture;
                RenderTextures.Release(ref texture);
                if (request.hasError)
                {
                    Volatile.Write(ref busy, 0);
                    return;
                }
                NativeArray<byte> data = request.GetData<byte>();
                int length = data.Length;
                if (buffer == null || buffer.Length < length) buffer = new byte[length];
                byte[] bytes = buffer;
                NativeArray<byte>.Copy(data, bytes, length);
                string path = next.Path;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    TryWrite(path, bytes, length);
                    Volatile.Write(ref busy, 0);
                });
            });
        }

        private static void TryWrite(string path, byte[] bytes, int length)
        {
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write))
                    file.Write(bytes, 0, length);
            }
            catch (System.Exception)
            {
                // A diagnostic: a file not written is a file missing from the folder.
            }
        }

        private static void Append(Matrix4x4 m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    meta.Append(' ').Append(m[r, c].ToString("R", CultureInfo.InvariantCulture));
        }

        private static void ReleasePrepared()
        {
            foreach (Dictionary<string, RenderTexture> frame in prepared)
            {
                foreach (RenderTexture texture in frame.Values)
                {
                    RenderTexture copy = texture;
                    RenderTextures.Release(ref copy);
                }
                frame.Clear();
            }
        }
    }
}
